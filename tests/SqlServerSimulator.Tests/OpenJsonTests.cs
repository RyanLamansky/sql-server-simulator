using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>OPENJSON(...) [WITH (...)]</c> rowset-
/// returning function. Covers the EF Core 10 primitive-collection
/// emissions (with and without WITH-clause) and a few raw-SQL shapes.
/// </summary>
[TestClass]
public sealed class OpenJsonTests
{
    [TestMethod]
    public void OpenJson_PrimitiveStringArray_DefaultSchema()
    {
        using var reader = new Simulation().ExecuteReader("select [key], [value], [type] from openjson('[\"a\",\"b\",\"c\"]')");
        var rows = new List<(string key, string value, int type)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        CollectionAssert.AreEqual(new[] { ("0", "a", 1), ("1", "b", 1), ("2", "c", 1) }, rows);
    }

    [TestMethod]
    public void OpenJson_PrimitiveIntArray_DefaultSchemaTypeCode()
    {
        using var reader = new Simulation().ExecuteReader("select [type] from openjson('[1, 2, 3]')");
        var types = new List<int>();
        while (reader.Read())
            types.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 2, 2, 2 }, types);
    }

    [TestMethod]
    public void OpenJson_ObjectInput_KeysAreProperties()
    {
        using var reader = new Simulation().ExecuteReader("select [key] from openjson('{\"a\":1, \"b\":2}')");
        var keys = new List<string>();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "a", "b" }, keys);
    }

    [TestMethod]
    public void OpenJson_NullJson_NoRows()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from openjson(null)"));

    /// <summary>
    /// A document that isn't JSON text raises Msg 13609 with OPENJSON's own
    /// State 4 — see <see cref="JsonMalformedTextTests"/> for the full rule.
    /// </summary>
    [TestMethod]
    public void OpenJson_InvalidJson_RaisesMsg13609()
        => new Simulation().AssertSqlError("select count(*) from openjson('not json')", 13609,
            "JSON text is not properly formatted. Unexpected character 'n' is found at position 0.");

    [TestMethod]
    public void OpenJson_WithSelfPath_PrimitiveCollection()
    {
        using var reader = new Simulation().ExecuteReader("select [v] from openjson('[\"x\",\"y\",\"z\"]') with ([v] nvarchar(max) '$')");
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "x", "y", "z" }, values);
    }

    [TestMethod]
    public void OpenJson_WithIntPath_TypeCoerces()
    {
        using var reader = new Simulation().ExecuteReader("select [v] from openjson('[10, 20, 30]') with ([v] int '$')");
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, values);
    }

    [TestMethod]
    public void OpenJson_WithObjectArray_ColumnsExtractByDefaultPath()
    {
        using var reader = new Simulation().ExecuteReader("select [Kind], [Number] from openjson('[{\"Kind\":\"work\",\"Number\":\"555-1\"}, {\"Kind\":\"home\",\"Number\":\"555-2\"}]') with ([Kind] nvarchar(20), [Number] nvarchar(50))");
        var rows = new List<(string kind, string number)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        CollectionAssert.AreEqual(new[] { ("work", "555-1"), ("home", "555-2") }, rows);
    }

    [TestMethod]
    public void OpenJson_WithExplicitPath()
    {
        using var reader = new Simulation().ExecuteReader("select [n] from openjson('[{\"score\":100}, {\"score\":200}]') with ([n] int '$.score')");
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 100, 200 }, values);
    }

    [TestMethod]
    public void OpenJson_DocPath_IntoArrayProperty()
    {
        using var reader = new Simulation().ExecuteReader("select [v] from openjson('{\"items\":[1,2,3]}', '$.items') with ([v] int '$')");
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, values);
    }

    [TestMethod]
    public void OpenJson_BoolTypeCoerces()
    {
        using var reader = new Simulation().ExecuteReader("select [v] from openjson('[true, false, true]') with ([v] bit '$')");
        var values = new List<bool>();
        while (reader.Read())
            values.Add(reader.GetBoolean(0));
        CollectionAssert.AreEqual(new[] { true, false, true }, values);
    }

    [TestMethod]
    public void OpenJson_DecimalTypeCoerces()
    {
        using var reader = new Simulation().ExecuteReader("select [v] from openjson('[1.5, 2.5]') with ([v] decimal(10, 2) '$')");
        var values = new List<decimal>();
        while (reader.Read())
            values.Add(reader.GetDecimal(0));
        CollectionAssert.AreEqual(new[] { 1.5m, 2.5m }, values);
    }

    [TestMethod]
    public void OpenJson_GuidTypeCoerces()
    {
        var guid = Guid.NewGuid();
        using var reader = new Simulation().ExecuteReader($"select [v] from openjson('[\"{guid}\"]') with ([v] uniqueidentifier '$')");
        IsTrue(reader.Read());
        AreEqual(guid, reader.GetGuid(0));
    }

    [TestMethod]
    public void OpenJson_NullElementSurfacesAsSqlNull()
    {
        using var reader = new Simulation().ExecuteReader("select [v] from openjson('[1, null, 3]') with ([v] int '$')");
        var values = new List<int?>();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? null : reader.GetInt32(0));
        CollectionAssert.AreEqual(new int?[] { 1, null, 3 }, values);
    }

    // EF Core 10's primitive-collection .Contains shape end-to-end:
    // OPENJSON inside an IN(SELECT) subquery, with the outer table and
    // a primitive-collection column.
    [TestMethod]
    public void OpenJson_EfPrimitiveContainsShape()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, tags nvarchar(max))");
        _ = simulation.ExecuteNonQuery("insert t values (1, '[\"alpha\",\"beta\"]'), (2, '[\"gamma\"]'), (3, '[\"beta\",\"delta\"]')");
        using var reader = simulation.ExecuteReader("""
            select id from t
            where N'beta' in (
                select [v] from openjson([t].[tags]) with ([v] nvarchar(max) '$')
            )
            order by id
            """);
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }

    // EF Core 10's primitive-collection .Count shape: OPENJSON in a
    // scalar subquery returning COUNT(*).
    [TestMethod]
    public void OpenJson_EfPrimitiveCountShape()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, scores nvarchar(max))");
        _ = simulation.ExecuteNonQuery("insert t values (1, '[10, 20, 30]'), (2, '[]'), (3, '[42]')");
        using var reader = simulation.ExecuteReader("""
            select id, (select count(*) from openjson([t].[scores])) as score_count
            from t order by id
            """);
        var rows = new List<(int id, int count)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 3), (2, 0), (3, 1) }, rows);
    }

    [TestMethod]
    public void OpenJson_AsJsonOnNonNVarcharMax_RaisesMsg13618()
        => new Simulation().AssertSqlError(
            "select * from openjson('[{\"a\":1}]') with (a int 'strict $.a' as json)",
            13618,
            "AS JSON option can be specified only for column of nvarchar(max) type in WITH clause.");

    [TestMethod]
    public void OpenJson_AsJson_ObjectSubtree_PreservesVerbatimText()
        => AreEqual("{  \"b\" : 2 , \"a\" : 1  }", new Simulation().ExecuteScalar(
            "select x from openjson('{ \"o\" : {  \"b\" : 2 , \"a\" : 1  } }') with (x nvarchar(max) '$.o' as json)"));

    [TestMethod]
    public void OpenJson_AsJson_ArraySubtree()
        => AreEqual("[1,2,3]", new Simulation().ExecuteScalar(
            "select x from openjson('{\"tags\":[1,2,3]}') with (x nvarchar(max) '$.tags' as json)"));

    [TestMethod]
    public void OpenJson_AsJson_ScalarUnderLax_Null()
    {
        using var reader = new Simulation().ExecuteReader(
            "select x from openjson('{\"scalar\":42}') with (x nvarchar(max) '$.scalar' as json)");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void OpenJson_AsJson_ScalarUnderStrict_RaisesMsg13624()
        => new Simulation().AssertSqlError(
            "select x from openjson('{\"scalar\":42}') with (x nvarchar(max) 'strict $.scalar' as json)",
            13624,
            "Object or array cannot be found in the specified JSON path.");

    [TestMethod]
    public void OpenJson_AsJson_MissingUnderStrict_RaisesMsg13608_State6()
    {
        var ex = new Simulation().AssertSqlError(
            "select x from openjson('{\"a\":1}') with (x nvarchar(max) 'strict $.missing' as json)",
            13608);
        AreEqual("Property cannot be found on the specified JSON path.", ex.Message);
        AreEqual((byte)6, ex.State);
    }

    [TestMethod]
    public void OpenJson_AsJson_JsonNullUnderStrict_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader(
            "select x from openjson('{\"n\":null}') with (x nvarchar(max) 'strict $.n' as json)");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void OpenJson_AsJson_MixedScalarAndSubtreeColumns()
    {
        using var reader = new Simulation().ExecuteReader("""
            select name, addr, city from openjson('{"name":"Alice","address":{"city":"NYC"}}')
            with (name nvarchar(100) '$.name', addr nvarchar(max) '$.address' as json, city nvarchar(50) '$.address.city')
            """);
        IsTrue(reader.Read());
        AreEqual("Alice", reader.GetString(0));
        AreEqual("{\"city\":\"NYC\"}", reader.GetString(1));
        AreEqual("NYC", reader.GetString(2));
    }

    [TestMethod]
    public void OpenJson_AsJson_ArraySource_ExtractsSubtreePerElement()
    {
        using var reader = new Simulation().ExecuteReader("""
            select id, meta from openjson('[{"id":1,"meta":{"x":10}},{"id":2,"meta":{"y":20}}]')
            with (id int '$.id', meta nvarchar(max) '$.meta' as json)
            """);
        var rows = new List<(int id, string meta)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        CollectionAssert.AreEqual(new[] { (1, "{\"x\":10}"), (2, "{\"y\":20}") }, rows);
    }

    // JSON_QUERY shares the AS JSON subtree-extraction rule: a strict-mode
    // scalar match raises Msg 13624 (lax returns NULL).
    [TestMethod]
    public void JsonQuery_StrictScalar_RaisesMsg13624()
        => new Simulation().AssertSqlError(
            "select json_query('{\"a\":1}', 'strict $.a')",
            13624,
            "Object or array cannot be found in the specified JSON path.");

    // EF Core 10's primitive-collection .Any() shape: EXISTS over a
    // typed-OPENJSON subquery with a WHERE filter.
    [TestMethod]
    public void OpenJson_EfPrimitiveAnyShape()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, scores nvarchar(max))");
        _ = simulation.ExecuteNonQuery("insert t values (1, '[10, 20]'), (2, '[5, 8]'), (3, '[100]')");
        using var reader = simulation.ExecuteReader("""
            select id from t where exists (
                select 1 from openjson([t].[scores]) with ([v] int '$') as [s]
                where [s].[v] > 15
            ) order by id
            """);
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }
}
