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

    [TestMethod]
    public void OpenJson_InvalidJson_LaxNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from openjson('not json')"));

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
