using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// A JSON object may name the same property twice. SQL Server's reader stops
/// at the first one it meets, so that is the property every path resolves to
/// and the one <c>JSON_MODIFY</c> edits; the later namesakes are text the
/// reader walks over. <c>OPENJSON</c>'s default schema unfolds rather than
/// resolving, so it reports every occurrence. All probe-confirmed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class JsonDuplicateKeyTests
{
    [TestMethod]
    public void JsonValue_ReadsTheFirst()
        => AreEqual("1", ExecuteScalar("select json_value('{\"a\":1,\"a\":2}', '$.a')"));

    [TestMethod]
    public void JsonValue_ReadsTheFirstOfThree()
        => AreEqual("1", ExecuteScalar("select json_value('{\"a\":1,\"b\":9,\"a\":2,\"a\":3}', '$.a')"));

    [TestMethod]
    public void JsonValue_StrictPath_ReadsTheFirst()
        => AreEqual("1", ExecuteScalar("select json_value('{\"a\":1,\"a\":2}', 'strict $.a')"));

    [TestMethod]
    public void JsonValue_NestedDuplicate_DescendsThroughTheFirst()
        => AreEqual("1", ExecuteScalar("select json_value('{\"a\":{\"b\":1},\"a\":{\"b\":2}}', '$.a.b')"));

    /// <summary>
    /// The first match binds even when it is the wrong shape to answer with:
    /// the reader has stopped, so the later namesake is never consulted.
    /// </summary>
    [TestMethod]
    [DataRow("json_value('{\"a\":{\"z\":1},\"a\":2}', '$.a')")]
    [DataRow("json_value('{\"a\":null,\"a\":2}', '$.a')")]
    [DataRow("json_query('{\"a\":1,\"a\":[2]}', '$.a')")]
    public void FirstMatchBinds_EvenWhenItAnswersNull(string expression)
        => IsInstanceOfType<DBNull>(ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void JsonQuery_ReadsTheFirst()
        => AreEqual("[1]", ExecuteScalar("select json_query('{\"a\":[1],\"a\":[2]}', '$.a')"));

    [TestMethod]
    public void JsonPathExists_Returns1()
        => AreEqual(1, ExecuteScalar("select json_path_exists('{\"a\":1,\"a\":2}', '$.a')"));

    /// <summary>A repeated name is well-formed JSON text.</summary>
    [TestMethod]
    public void IsJson_Returns1()
        => AreEqual(1, ExecuteScalar("select isjson('{\"a\":1,\"a\":2}')"));

    /// <summary>
    /// OPENJSON's default schema unfolds the object member by member, so both
    /// occurrences arrive as their own rows.
    /// </summary>
    [TestMethod]
    public void OpenJson_DefaultSchema_EmitsEveryOccurrence()
    {
        var simulation = new Simulation();
        AreEqual(3, simulation.ExecuteScalar("select count(*) from openjson('{\"a\":1,\"a\":2,\"b\":3}')"));
        AreEqual("1,2", simulation.ExecuteScalar(
            "select string_agg([value], ',') from openjson('{\"a\":1,\"a\":2,\"b\":3}') where [key] = 'a'"));
    }

    /// <summary>A WITH-clause column resolves a path, so it reads the first.</summary>
    [TestMethod]
    public void OpenJson_WithClause_ReadsTheFirst()
        => AreEqual(1, ExecuteScalar("select a from openjson('{\"a\":1,\"a\":2}') with (a int '$.a')"));

    [TestMethod]
    public void JsonModify_ReplacesTheFirstAndLeavesTheOther()
        => AreEqual("{\"a\":9,\"a\":2}", ExecuteScalar("select json_modify('{\"a\":1,\"a\":2}', '$.a', 9)"));

    [TestMethod]
    public void JsonModify_StrictPath_ReplacesTheFirst()
        => AreEqual("{\"a\":9,\"a\":2}", ExecuteScalar("select json_modify('{\"a\":1,\"a\":2}', 'strict $.a', 9)"));

    [TestMethod]
    public void JsonModify_DeletesTheFirst()
        => AreEqual("{\"a\":2}", ExecuteScalar("select json_modify('{\"a\":1,\"a\":2}', '$.a', null)"));

    [TestMethod]
    public void JsonModify_DescendsThroughTheFirst()
        => AreEqual("{\"a\":{\"b\":9},\"a\":{\"b\":2}}",
            ExecuteScalar("select json_modify('{\"a\":{\"b\":1},\"a\":{\"b\":2}}', '$.a.b', 9)"));

    [TestMethod]
    public void JsonModify_AppendsOntoTheFirst()
        => AreEqual("{\"a\":[1,9],\"a\":[2]}",
            ExecuteScalar("select json_modify('{\"a\":[1],\"a\":[2]}', 'append $.a', 9)"));

    /// <summary>An insert still lands at the closing brace, past both.</summary>
    [TestMethod]
    public void JsonModify_InsertGoesAfterBoth()
        => AreEqual("{\"a\":1,\"a\":2,\"b\":9}", ExecuteScalar("select json_modify('{\"a\":1,\"a\":2}', '$.b', 9)"));
}
