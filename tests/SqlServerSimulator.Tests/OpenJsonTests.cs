using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

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
        var rows = new List<(string key, string value, byte type)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetByte(2)));
        CollectionAssert.AreEqual(new[] { ("0", "a", (byte)1), ("1", "b", (byte)1), ("2", "c", (byte)1) }, rows);
    }

    [TestMethod]
    public void OpenJson_PrimitiveIntArray_DefaultSchemaTypeCode()
    {
        using var reader = new Simulation().ExecuteReader("select [type] from openjson('[1, 2, 3]')");
        // The type code is tinyint, as on real (probe-confirmed 2026-09-23).
        var types = new List<byte>();
        while (reader.Read())
            types.Add(reader.GetByte(0));
        CollectionAssert.AreEqual(new byte[] { 2, 2, 2 }, types);
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

    [TestMethod]
    [DataRow("select * from openjson(N'{\"b\":1}', 'strict $.a')", 13608, 3)]
    [DataRow("select * from openjson(N'{\"a\":[1]}', 'strict $.a[3]')", 13608, 3)]
    [DataRow("select * from openjson(N'{\"a\":1}', 'strict $.a')", 13611, 1)]
    [DataRow("select * from openjson(N'{\"a\":null}', 'strict $.a')", 13611, 1)]
    [DataRow("select * from openjson(N'{\"a\":\"x\"}', 'strict $.a') with (v int '$')", 13611, 2)]
    public void StrictDocumentPath_RaisesWhereLaxOpensNothing(string query, int number, int state)
    {
        AreEqual(state, new Simulation().AssertSqlError(query, number).State);
        using var reader = new Simulation().ExecuteReader(query.Replace("strict", "lax", StringComparison.Ordinal));
        IsFalse(reader.Read());
    }

    // ---- probed 2026-10-02 against SQL Server 2025 ----

    [TestMethod]
    public void DefaultSchema_KeyPast4000Characters_IsCut()
        => AreEqual(4000, ExecuteScalar("select len([key]) from openjson('{\"' + replicate('k', 4001) + '\":1}')"));

    [TestMethod]
    [DataRow("select * from openjson('{\"a\":[1]}', null)")]
    [DataRow("declare @p nvarchar(20); select * from openjson('{\"a\":[1]}', @p)")]
    public void NullDocumentPath_IsMsg8116State9(string sql)
    {
        var error = new Simulation().AssertSqlError(sql, 8116);
        AreEqual("Argument data type NULL is invalid for argument 2 of OPENJSON function.", error.Errors[0].Message);
        AreEqual((byte)9, error.Errors[0].State);
    }

    [TestMethod]
    public void NonStringDocument_IsReadAsTheNVarcharItConvertsTo()
    {
        var sim = new Simulation();
        AreEqual("JSON text is not properly formatted. Unexpected character '1' is found at position 0.", sim.AssertSqlError("select * from openjson(1)", 13609).Errors[0].Message);
        AreEqual("2", sim.ExecuteScalar("select [value] from openjson(cast(N'[2]' as varbinary(20)))"));
    }

    [TestMethod]
    [DataRow("[{\"v\":true}]", "v nvarchar(10)", "true")]
    [DataRow("[{\"v\":\"abcdef\"}]", "v nvarchar(3)", "abc")]
    [DataRow("[{\"v\":\"abcdef\"}]", "v varchar(3) 'strict $.v'", "abc")]
    [DataRow("[{\"v\":\"abcdef\"}]", "v nvarchar", "a")]
    [DataRow("[{\"v\":12345}]", "v varchar(3)", "123")]
    [DataRow("[{\"v\":\"ab\"}]", "v char(4)", "ab  ")]
    public void WithColumn_CharacterTarget_TakesTheTextCutToLength(string document, string column, string expected)
        => AreEqual(expected, ExecuteScalar($"select v from openjson('{document}') with ({column})"));

    [TestMethod]
    public void WithColumn_TrueIntoInt_IsAConversionFailureNamingTheWord()
        => new Simulation().AssertSqlError("select * from openjson('[{\"v\":true}]') with (v int)", 245, "Conversion failed when converting the nvarchar value 'true' to data type int.");

    [TestMethod]
    public void WithColumn_Binary_ReadsBase64()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, (byte[])sim.ExecuteScalar("select v from openjson('[{\"v\":\"AQID\"}]') with (v varbinary(10))")!);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 0, 0 }, (byte[])sim.ExecuteScalar("select v from openjson('[{\"v\":\"AQID\"}]') with (v binary(5))")!);
        _ = sim.AssertSqlError("select * from openjson('[{\"v\":\"x\"}]') with (v varbinary(10))", 13612);
        AreEqual((byte)1, sim.AssertSqlError("select * from openjson('[{\"v\":\"AQIDBA==\"}]') with (v varbinary(2))", 13613).Errors[0].State);
        AreEqual((byte)2, sim.AssertSqlError("select * from openjson('[{\"v\":\"x\"}]') with (v rowversion)", 13613).Errors[0].State);
    }

    [TestMethod]
    [DataRow("text", 13614)]
    [DataRow("ntext", 13614)]
    [DataRow("image", 13614)]
    [DataRow("sql_variant", 13614)]
    [DataRow("hierarchyid", 13616)]
    [DataRow("geometry", 13616)]
    public void WithColumn_TypesTheReaderCantProduce_AreRefused(string type, int number)
        => new Simulation().AssertSqlError($"select * from openjson('[{{\"v\":1}}]') with (v {type})", number);

    [TestMethod]
    public void WithColumn_ScalarOverAContainer_IsNullOrStrictMsg13624()
    {
        var sim = new Simulation();
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select v from openjson('[{\"v\":[1,2]}]') with (v nvarchar(20))"));
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select v from openjson('[{\"v\":{\"a\":1}}]') with (v int)"));
        _ = sim.AssertSqlError("select * from openjson('[{\"v\":[1]}]') with (v int 'strict $.v')", 13624);
    }

    [TestMethod]
    public void WithColumn_DefaultPath_QuotesTheColumnName()
        => AreEqual("5|6", ExecuteScalar("select concat([a b], '|', [c\"d]) from openjson('[{\"a b\":5,\"c\\\"d\":6}]') with ([a b] int, [c\"d] int)"));

    [TestMethod]
    public void WithColumn_Collation_IsTheClausesOrTheDocuments()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar("select count(*) from openjson('[{\"v\":\"a\"}]') with (v nvarchar(10) collate Latin1_General_CS_AS) where v = 'A'"));
        AreEqual("Latin1_General_CS_AS", sim.ExecuteScalar("select sql_variant_property(v, 'Collation') from openjson('[{\"v\":\"x\"}]' collate Latin1_General_CS_AS) with (v nvarchar(10))"));
        AreEqual("Latin1_General_CS_AS", sim.ExecuteScalar("select sql_variant_property(cast([value] as nvarchar(10)), 'Collation') from openjson('[1]' collate Latin1_General_CS_AS)"));
        _ = sim.AssertSqlError("select * from openjson('[{\"v\":1}]') with (v int collate Latin1_General_BIN)", 447);
    }

    [TestMethod]
    public void WithColumn_DecimalConversionFailure_NamesTheTypeAsWritten()
    {
        var sim = new Simulation();
        sim.AssertSqlError("select * from openjson('[{\"v\":1e3}]') with (v decimal(10,2))", 8114, "Error converting data type nvarchar to decimal.");
        sim.AssertSqlError("select * from openjson('[{\"v\":\"x\"}]') with (v numeric(5,1))", 8114, "Error converting data type nvarchar to numeric.");
    }

    [TestMethod]
    public void View_KeyColumn_ReportsItsBinaryCollation()
        => AreEqual("Latin1_General_BIN2", new Simulation().ExecuteBatchesScalar(
            "create view v as select [key], [value] from openjson('[1,2]')",
            "select collation_name from sys.columns where object_id = object_id('v') and name = 'key'"));
}
